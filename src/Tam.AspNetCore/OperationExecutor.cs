using System.Reflection;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Tam.EntityFrameworkCore;

namespace Tam.AspNetCore;

/// <summary>
/// The execution pipeline (docs/09): authorization → idempotency → structural validation →
/// transaction → handler → extension channel → audit capture → save → response. Every caller
/// — web, MCP, integration, test — comes through here.
/// </summary>
public sealed class OperationExecutor(
    TamModel model,
    IServiceProvider services,
    Func<IServiceProvider, DbContext> dbResolver)
{
    /// <summary>The sanctioned path for a NON-request caller — an integration, a job, internal
    /// dispatch — to run ONE operation as its OWN unit of work: a fresh pinned scope + DbContext +
    /// transaction (Sol re-review, Finding 1). Each invocation is isolated, so a blocked
    /// operation's writes can never flush onto a caller's shared context or the next unit. The
    /// instance <see cref="ExecuteAsync"/> runs on whatever DbContext its scope provides — correct
    /// on the request path (one operation per request scope, disposed after) — but a caller that
    /// runs SEVERAL operations, or saves unrelated state afterward, MUST come through here rather
    /// than resolve one executor and reuse it. <paramref name="prepare"/> runs INSIDE the pinned
    /// scope: build the scoped context (Services = the scope) and map the body there, so mapping
    /// and execution share the isolated unit.</summary>
    public static async Task<OperationResponse> ExecuteIsolatedAsync(
        IServiceProvider rootServices, string tenant, string operationId,
        Func<IServiceProvider, CancellationToken, Task<(JsonElement Body, OperationContext Context)>> prepare,
        CancellationToken ct)
    {
        return await PinnedScope.RunAsync<OperationResponse>(rootServices, tenant,
            async (IServiceProvider scoped, CancellationToken sct) =>
            {
                var prepared = await prepare(scoped, sct);
                return await scoped.GetRequiredService<OperationExecutor>()
                    .ExecuteAsync(operationId, prepared.Body, prepared.Context, sct);
            }, ct);
    }

    /// <remarks>Runs on the DbContext of the scope that resolved this executor. On the request
    /// path that scope holds exactly one operation and is disposed after, so its unit of work is
    /// already isolated. A non-request caller that reuses one scope across operations must use
    /// <see cref="ExecuteIsolatedAsync"/> instead — though a blocked attempt here now also clears
    /// its own tracked writes, so a stray shared-context save cannot flush them (Finding 1).</remarks>
    public async Task<OperationResponse> ExecuteAsync(
        string operationId, JsonElement body, OperationContext context, CancellationToken ct,
        string? formId = null)
    {
        if (!model.Operations.TryGetValue(operationId, out var operation))
            return Fail(context, PipelineFindings.UnknownOperation.With(("operation", operationId)));

        // An absent body (a default/Undefined JsonElement — e.g. an MCP tools/call with no
        // "arguments") would throw at the first GetRawText below. Normalize it to an invalid-input
        // finding here so EVERY caller — HTTP, MCP, integration, internal — fails closed, not with
        // an unhandled exception (Sol re-review, boundary A/MCP-B).
        if (body.ValueKind == JsonValueKind.Undefined)
            return Fail(context, PipelineFindings.InvalidInput.Create());

        // Inactive plugin → the operation does not exist for this tenant (docs/22): checked
        // before authorization so the answer is indistinguishable from an unknown id.
        if (!await ActivationCache.ContributionExistsAsync(
                services, dbResolver(services), operation.Plugin, context.TenantId.Value, ct))
            return Fail(context, PipelineFindings.UnknownOperation.With(("operation", operationId)));

        if (!context.Actor.Can(operation.Permission))
            return Fail(context, PipelineFindings.NotAuthorized.With(("permission", operation.Permission)));

        // Write masking (docs/27 D-A3): an input that carries a sensitive field the actor may not
        // touch is rejected outright — the masked manifest never offered the field, so its presence
        // is either a stale client or a forged request. Checked on the raw body, before binding.
        var maskedWrites = operation.InputFields
            .Where(f => f.IsMaskedFor(context.Actor))
            .Where(f => body.ValueKind == JsonValueKind.Object && body.TryGetProperty(f.WireName, out _))
            .Select(f => PipelineFindings.FieldNotAuthorized
                .With(("field", f.WireName), ("permission", f.SensitivePermission!)).At(f.MemberName))
            .ToList();
        if (maskedWrites.Count > 0)
            return Fail(context, [.. maskedWrites]);

        var db = dbResolver(services);

        // Resolve the selected form binding BEFORE anything downstream (Sol re-review, Finding 3):
        // once a caller supplies ?form=, it is "execute through EXACTLY this binding or reject" — a
        // form is authoritative tightening, not a hint applied only when it happens to be valid. A
        // supplied form must exist, be bound to THIS operation, and be contributed by a plugin active
        // for the tenant — otherwise a typo'd or wrong-operation id would silently downgrade to the
        // direct contract, or an inactive plugin's form would still tighten a host operation.
        FormDefinition? selectedForm = null;
        if (formId is not null)
        {
            if (!model.Forms.TryGetValue(formId, out selectedForm))
                return Fail(context, PipelineFindings.UnknownForm.With(("form", formId)));
            if (selectedForm.OperationId != operation.Id)
                return Fail(context, PipelineFindings.InvalidInput
                    .With(("form", formId), ("operation", operation.Id)));
            if (!await ActivationCache.ContributionExistsAsync(
                    services, db, selectedForm.Plugin, context.TenantId.Value, ct))
                return Fail(context, PipelineFindings.UnknownForm.With(("form", formId)));
        }

        var payloadHash = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(
            System.Text.Encoding.UTF8.GetBytes(body.GetRawText())));

        // The selected form is part of the effective request contract, so it is part of idempotency
        // identity (Sol re-review, Finding 2): a direct call and a through-a-stricter-form call with
        // the SAME body + key are DIFFERENT effective requests and must not replay each other's
        // outcome. Folding the binding into the stored key makes them independent idempotency records
        // (each still replays its own prior outcome, and a payload mismatch within one is still a
        // client bug), rather than colliding into one row that replays the wrong contract's result.
        var binding = selectedForm is null ? "op" : "form:" + selectedForm.Id;

        // Idempotency is scoped to the actor as well as tenant+operation: a replay must return the
        // caller's OWN prior outcome, never another actor's stored response (whose output/version
        // may reflect a different ownership scope). Different actor + same key = independent record.
        if (context.IdempotencyKey is { } key)
        {
            var scopedKey = context.Actor.Id + ":" + binding + ":" + key;
            var replay = await db.Set<IdempotencyRecord>().FindAsync(
                [context.TenantId.Value, operationId, scopedKey], ct);
            if (replay is not null)
            {
                // Same key + same payload → replay the stored outcome. Different payload → client bug.
                if (replay.PayloadHash != payloadHash)
                    return Fail(context, Finding.Error("pipeline.idempotency-mismatch").Create());
                var stored = JsonSerializer.Deserialize<OperationResponse>(replay.ResponseJson, TamJson.Options)!;
                return stored with
                {
                    Findings = [.. stored.Findings,
                        model.Locales.Resolve(PipelineFindings.IdempotentReplay.Create(), context.Culture)],
                };
            }
        }

        object? input;
        try
        {
            input = body.Deserialize(operation.InputType, TamJson.Options)
                ?? throw new JsonException("null input");
        }
        catch (JsonException)
        {
            return Fail(context, PipelineFindings.InvalidInput.Create());
        }

        var structural = ValidateStructural(operation, input);
        // Only the SELECTED form's requiredness tightening applies (docs/40) — a direct/MCP/
        // integration call (formId null) is bound by the operation contract alone, never by a
        // union of every form's RequiredWhen. The form is already validated + activation-checked.
        structural.AddRange(SelectedFormRequired(operation, input, selectedForm));
        if (structural.Any(f => f.Severity == FindingSeverity.Error))
            return Fail(context, [.. structural]);

        // Operation-owned AUTHORITATIVE requiredness (docs/40): run the operation's derivations and
        // enforce their Require() outputs for EVERY caller — the canonical contract, with the
        // domain-specific finding preserved (e.g. orders.project-required). Advisory outputs
        // (suggestions, warnings, option ordering) are consumed by resolve and IGNORED here:
        // authority is a property of the output, not of the derivation method.
        // Derivations run structurally read-only and self-contained (Sol re-review, Finding 3): the
        // write-guard interceptor rejects any durable write and RunDerivationsAsync discards any
        // unsaved tracked mutation, on every exit path — so submit never inherits derivation writes.
        // The change set a derivation reads via DerivationContext.WasChanged (Sol re-review round 6,
        // F3): at submit it is exactly the fields the body carries — a present field was submitted,
        // an absent one was not (edit forms send only touched change fields).
        var submittedFields = body.ValueKind == JsonValueKind.Object
            ? body.EnumerateObject().Select(p => p.Name).ToHashSet(StringComparer.Ordinal)
            : NoChangedFields;
        var derived = await RunDerivationsAsync(operation, input, context, ct, submittedFields);

        var required = derived.Required
            .Where(r => r.When && FieldEmpty(operation, input, r.Field))
            .Select(r => r.Finding).ToList();
        if (required.Count > 0)
            return Fail(context, [.. required]);

        // Blocking derivation findings are AUTHORITATIVE at submit (docs/40, Sol re-review Finding 1):
        // a derivation that emits an Error finding (AddFieldError / From / Add) admissibility-rejects
        // the input for EVERY caller — direct HTTP, MCP, integration — not just the resolve preview.
        // Advisory outputs (warnings, suggestions, option ordering) are non-blocking and pass through.
        // Without this the contract's "blocking findings BLOCK at submit" row was unenforced, relying
        // on a handler to independently repeat the check.
        var blockingDerived = derived.Findings
            .Where(f => f is { Severity: FindingSeverity.Error, BlocksSubmission: true })
            .ToList();
        if (blockingDerived.Count > 0)
            return Fail(context, [.. blockingDerived]);

        // Lookup membership (docs/40): a submitted non-null value must EXIST in the derivation's
        // candidate universe (view + base filters), checked by an Exists — never by the client's
        // browsed page. Authoritative for every caller. The key is read from the DESERIALIZED input,
        // not the raw JSON (Sol re-review, Finding 9): a lookup-bound edit field arrives as a
        // Change<T> object ({original,value}) on the wire, whose ToString() is the object, not the
        // key — reading the bound value unwraps the change set and the semantic wrapper, and matches
        // exactly the value the handler will act on.
        if (services.GetService<ViewExecutor>() is { } viewExecutor)
            foreach (var lookup in derived.Lookups)
            {
                var value = LookupKey(operation, input, lookup.Field);
                if (string.IsNullOrEmpty(value)) continue;
                if (!await viewExecutor.ContainsAsync(lookup.ViewId, lookup.Filters, value, context, ct))
                    return Fail(context, lookup.Invalid);
            }

        // Closed inline options (docs/40, Sol re-review Finding 7): the small-set twin of lookup
        // membership. A submitted value must be one of the derivation's authoritative options — the
        // complete legal set — else it is rejected. AddOptions (advisory) stays unenforced.
        foreach (var closed in derived.ClosedOptions)
        {
            var field = operation.InputFields.FirstOrDefault(f => f.WireName == closed.Field);
            var submitted = EffectiveScalar(operation, input, closed.Field);
            if (field is null || submitted is null) continue;
            // Normalize BOTH the submitted value and each option through the FIELD's semantic model
            // (Sol re-review, Finding 5) — not a global ToString — so decimal 1 / 1.0, an enum's wire
            // token vs CLR value, and equivalent date/reference forms compare correctly, a
            // case-sensitive code isn't accepted in the wrong casing, and a wrapper option reduces to
            // its scalar. One place, too, to reject an option value incompatible with the field type.
            var normalized = field.Semantic.Normalize(submitted);
            if (!closed.Options.Any(o =>
                    Equals(field.Semantic.Normalize(ValueWrapper.Unwrap(o.Value)), normalized)))
                return Fail(context, closed.Invalid);
        }

        // Plugin gates (docs/22 P2): typed preconditions in TWO phases, run only for tenants
        // with the owning plugin active; within each phase, operation-specific gates run before
        // wildcard gates (docs/28 approvals seam 1 — a wildcard gate sees every operation and
        // decides from its own config). Non-blocking findings a passing gate returns (warnings)
        // are carried into the response. Wire input only, never host CLR types.
        var specificGates = model.Gates.GetValueOrDefault(operationId, []);
        var wildcardGates = operationId == GateDefinition.Wildcard
            ? []
            : model.Gates.GetValueOrDefault(GateDefinition.Wildcard, []);
        var orderedGates = specificGates.Concat(wildcardGates).ToList();
        var gateFindings = new List<Finding>();
        IReadOnlySet<string> activePlugins = new HashSet<string>();
        ITamActivator activator = new TamActivator(services);
        if (orderedGates.Count > 0)
        {
            activePlugins = await ActivationCache.ForAsync(services, db, context.TenantId.Value, ct);
            activator = services.GetService(typeof(ITamActivator)) as ITamActivator ?? activator;
        }

        // PURE phase: gates declared pure-over-input run BEFORE the transaction — the cheap
        // fail. Tenant automation rules (docs/22 P5) ride here as tam.rules' wildcard gate.
        foreach (var gate in orderedGates.Where(g => g.Pure && activePlugins.Contains(g.PluginId)))
        {
            var gateContext = new GateContext(operationId, body, payloadHash, context);
            StampPlugin(gate.PluginId);
            var gateResult = await ((IOperationGate)activator.Create(gate.HandlerType))
                .CheckAsync(gateContext, ct);
            if (!gateResult.IsError)
            {
                gateFindings.AddRange(gateResult.Findings);
                continue;
            }
            // Nothing to roll back yet; parked work still commits from its own fresh scope.
            foreach (var work in gateContext.ParkedWork)
                await PinnedScope.RunAsync(services, context.TenantId.Value, work, ct);
            return Fail(context, [.. gateResult.Findings]);
        }

        await using var transaction = await db.Database.BeginTransactionAsync(ct);

        // TRANSACTIONAL phase: these gates run inside the operation's transaction, so their
        // reads and the handler's writes commit atomically. NOTE (Sol review, Finding 3): this
        // is NOT a frozen-read guarantee. The transaction opens at the provider default
        // (READ COMMITTED on Npgsql) and takes no lock on a row a gate merely READ, so a
        // concurrent commit to that row between the gate's read and this commit is invisible
        // unless the handler also writes that row (whose IVersioned token then conflicts). A
        // gate needing true freeze must take an explicit lock/version lease on the state it
        // depends on — a declared-lease seam is the designed follow-up.
        foreach (var gate in orderedGates.Where(g => !g.Pure && activePlugins.Contains(g.PluginId)))
        {
            var gateContext = new GateContext(operationId, body, payloadHash, context);
            StampPlugin(gate.PluginId);
            var handler = (IOperationGate)activator.Create(gate.HandlerType);
            var gateResult = await handler.CheckAsync(gateContext, ct);
            if (!gateResult.IsError)
            {
                gateFindings.AddRange(gateResult.Findings);
                continue;
            }

            // Parking (docs/28 approvals seam 2): the blocking gate may have deferred writes
            // — the envelope it wants to keep. Everything else about this attempt rolls back
            // FIRST; the parked work then runs in a fresh scope pinned to the same tenant, so
            // its commit is independent of the discarded attempt. A parking failure propagates:
            // answering "parked for approval" without a durable envelope would be a lie.
            await transaction.RollbackAsync(ct);
            db.ChangeTracker.Clear();   // discard this attempt's tracked writes (Sol re-review, Finding 1)
            foreach (var work in gateContext.ParkedWork)
                await PinnedScope.RunAsync(services, context.TenantId.Value, work, ct);
            return Fail(context, [.. gateResult.Findings]);
        }

        Result result;
        try
        {
            // The handler runs on behalf of the operation's OWNING plugin (null for host
            // operations) — never a gate that happened to run before it. Clearing the stamp
            // here is what keeps the plugin-scoped seams (docs/31) structurally honest.
            StampPlugin(operation.Plugin);
            result = await InvokeHandler(operation, input, context, ct);
        }
        catch (DbUpdateConcurrencyException)
        {
            return Fail(context, ConcurrencyFindings.VersionConflict.Create());
        }

        var findings = new List<Finding>(structural.Where(f => f.Severity != FindingSeverity.Error));
        findings.AddRange(gateFindings);
        findings.AddRange(result.Findings);
        var conflicts = new List<FieldConflict>(result.Conflicts ?? []);

        // Tenant extension channel: validate + apply against the tracked extensible aggregate.
        if (operation.ExtensibleEntity is { } extensibleType &&
            body.TryGetProperty("extensions", out var extensionsElement) &&
            extensionsElement.ValueKind == JsonValueKind.Object)
        {
            var changes = extensionsElement.Deserialize<Dictionary<string, ExtensionChange>>(TamJson.Options)!;
            if (changes.Count > 0)
            {
                // Deterministic target, never a guess: with ONE tracked instance of the extensible
                // type it is the target; with several (a handler that also loaded sibling rows) the
                // single Added/Modified one is; anything else fails CLOSED — silently writing onto
                // whichever row happened to be tracked first would be cross-record corruption.
                var candidates = db.ChangeTracker.Entries()
                    .Where(e => extensibleType.IsInstanceOfType(e.Entity))
                    .ToList();
                var written = candidates
                    .Where(e => e.State is EntityState.Added or EntityState.Modified)
                    .ToList();
                var tracked = candidates.Count == 1 ? candidates[0]
                    : written.Count == 1 ? written[0]
                    : null;
                if (tracked is null && candidates.Count > 1)
                    findings.Add(PipelineFindings.AmbiguousExtensionTarget.Create());
                if (tracked?.Entity is IExtensible extensible)
                {
                    // The registered registry, not a bare EF one: plugin-packaged fields
                    // (docs/22 P2) must validate exactly like tenant-defined fields.
                    var registry = services.GetService(typeof(IExtensionRegistry)) as IExtensionRegistry
                        ?? new EfExtensionRegistry(db);
                    var specs = await registry.For(
                        context.TenantId, TamModel.EntityKey(extensibleType), ct);
                    var applied = ExtensionApplier.Apply(
                        extensible, tracked.State == EntityState.Added, changes, specs);
                    findings.AddRange(applied.Findings);
                    conflicts.AddRange(applied.Conflicts);
                }
            }
        }

        var blocked = result.IsError
            || conflicts.Count > 0
            || findings.Any(f => f is { Severity: FindingSeverity.Error, BlocksSubmission: true });

        if (blocked)
        {
            await transaction.RollbackAsync(ct);
            db.ChangeTracker.Clear();   // discard this attempt's tracked writes (Sol re-review, Finding 1)
            return new OperationResponse(
                null,
                Resolve(findings, context),
                [],
                null,
                null,
                conflicts.Count > 0 ? conflicts : null);
        }

        // Outbox (docs/09): explicit event effects persist in this same transaction —
        // the event exists if and only if the operation committed.
        foreach (var effect in result.Effects.OfType<EventPublished>())
            db.Publish(effect.Event, effect.Payload, operationId, context.TenantId.Value);

        var audit = TamAudit.Capture(db, context, operationId);
        try
        {
            await db.SaveChangesAsync(ct);
        }
        catch (DbUpdateConcurrencyException) when (ct.IsCancellationRequested is false)
        {
            // A genuine optimistic-concurrency conflict on a versioned row this operation wrote.
            await transaction.RollbackAsync(ct);
            db.ChangeTracker.Clear();   // discard this attempt's tracked writes (Sol re-review, Finding 1)
            return Fail(context, ConcurrencyFindings.VersionConflict.Create());
        }
        catch (DbUpdateException ex) when (ct.IsCancellationRequested is false
            && services.GetService<ITamDbErrorClassifier>()?.IsUniqueViolation(ex) == true)
        {
            // A check-then-insert race lost to a UNIQUE index (two concurrent installs,
            // activations, definitions). The serial path would have produced a duplicate
            // finding; surface the same conflict shape instead of a 500. Every OTHER
            // DbUpdateException — FK, check, not-null, conversion, provider/connection fault —
            // is a real failure and propagates uncaught rather than masquerading as a retryable
            // version conflict (Sol review, Finding 4).
            await transaction.RollbackAsync(ct);
            db.ChangeTracker.Clear();   // discard this attempt's tracked writes (Sol re-review, Finding 1)
            return Fail(context, ConcurrencyFindings.VersionConflict.Create());
        }

        var effects = result.Effects.Concat(TamAudit.InferEffects(audit)).ToList();
        var version = db.ChangeTracker.Entries()
            .Select(e => e.Entity).OfType<IVersioned>()
            .Select(v => (long?)v.Version).FirstOrDefault();

        var response = new OperationResponse(
            Output(result),
            Resolve(findings, context),
            effects.Select(e => (object)e).ToList(),
            version,
            audit.Id.ToString("N"),
            null);

        if (context.IdempotencyKey is { } storeKey)
        {
            var scopedStoreKey = context.Actor.Id + ":" + binding + ":" + storeKey;
            db.Add(new IdempotencyRecord
            {
                Key = scopedStoreKey,
                TenantId = context.TenantId.Value,
                OperationId = operationId,
                PayloadHash = payloadHash,
                ResponseJson = JsonSerializer.Serialize(response, TamJson.Options),
                Timestamp = DateTimeOffset.UtcNow,
            });
            try
            {
                await db.SaveChangesAsync(ct);
            }
            catch (DbUpdateException ex) when (ct.IsCancellationRequested is false
                && services.GetService<ITamDbErrorClassifier>()?.IsUniqueViolation(ex) == true)
            {
                // Concurrent identical request won the idempotency-key UNIQUE race: this attempt
                // rolls back entirely and the stored outcome is replayed (or rejected on mismatch).
                // Any OTHER DbUpdateException here — connection fault, FK, check, not-null — is a
                // real failure and propagates rather than masquerading as an idempotency race
                // (Sol re-review, boundary 4B: mirror the main save path's classifier guard).
                await transaction.RollbackAsync(ct);
                db.ChangeTracker.Clear();
                var winner = await db.Set<IdempotencyRecord>().FindAsync(
                    [context.TenantId.Value, operationId, scopedStoreKey], ct);
                if (winner is null || winner.PayloadHash != payloadHash)
                    return Fail(context, Finding.Error("pipeline.idempotency-mismatch").Create());
                var stored = JsonSerializer.Deserialize<OperationResponse>(winner.ResponseJson, TamJson.Options)!;
                return stored with
                {
                    Findings = [.. stored.Findings,
                        model.Locales.Resolve(PipelineFindings.IdempotentReplay.Create(), context.Culture)],
                };
            }
        }

        await transaction.CommitAsync(ct);

        // Live-refresh fan-out through the backplane: local subscribers immediately, and every other
        // instance via the configured transport (Postgres NOTIFY). Carries the entity effects grids
        // refresh on — those exist only here, not in the event outbox.
        (services.GetService(typeof(IEffectBackplane)) as IEffectBackplane)
            ?.Send(context.TenantId.Value, operationId, response.Effects);

        return response;
    }

    /// <summary>Form-specific validation TIGHTENING (docs/40): when a submission comes THROUGH a
    /// named form binding, that form's RequiredWhen predicates apply on top of the operation's own
    /// contract. Only the SELECTED form is consulted — never a union of every form bound to the
    /// operation, which was the round-1 fix's flaw: one presentation binding could tighten every
    /// other caller's contract, including direct/MCP/integration callers. The form arrives already
    /// resolved, operation-matched and activation-checked (Sol re-review, Finding 3); a direct call
    /// (no form) applies none of this — the operation's derivations and handlers govern there.</summary>
    private static List<Finding> SelectedFormRequired(OperationDefinition operation, object input, FormDefinition? form)
    {
        var findings = new List<Finding>();
        if (form is null) return findings;

        object? FieldValue(string wireName) => EffectiveFieldValue(operation, input, wireName);

        foreach (var config in form.Fields)
        {
            if (config.RequiredWhen is not { } predicate
                || !PxBinary.Truthy(predicate.Evaluate(FieldValue))) continue;
            if (operation.InputFields.Any(f => f.WireName == config.WireName)
                && FieldEmpty(operation, input, config.WireName))
                findings.Add(ValidationFindings.Required.At(config.WireName));
        }
        return findings;
    }

    /// <summary>Structural validation from the model: requiredness by nullability, semantic type rules.</summary>
    private static List<Finding> ValidateStructural(OperationDefinition operation, object input)
    {
        var findings = new List<Finding>();
        foreach (var field in operation.InputFields)
        {
            var property = ReflectionCache.Property(operation.InputType, field.MemberName);
            var value = property.GetValue(input);

            if (field.Required && IsEmpty(value))
            {
                findings.Add(ValidationFindings.Required.At(field.WireName));
                continue;
            }

            var toValidate = field.IsChangeSet && value is not null
                ? ReflectionCache.Property(value.GetType(), "Value").GetValue(value)
                : value;
            if (toValidate is null) continue;

            var normalized = field.Semantic.Normalize(ValueWrapper.Unwrap(toValidate));
            if (field.Semantic.Validate(normalized) is { } invalid)
                findings.Add(invalid.At(field.WireName));
        }
        return findings;
    }

    /// <summary>Marks the CURRENT scope's handler construction as owned by a plugin, so the
    /// plugin-scoped seams (field writer, host view reader — docs/31) enforce structurally.</summary>
    private void StampPlugin(string? pluginId)
    {
        if (services.GetService(typeof(PluginContext)) is PluginContext plugin)
            plugin.PluginId = pluginId;
    }

    private static bool IsEmpty(object? value)
    {
        var unwrapped = ValueWrapper.Unwrap(value);
        return unwrapped switch
        {
            null => true,
            string s => s.Length == 0,
            Guid g => g == Guid.Empty,
            _ => false,
        };
    }

    /// <summary>Generator-wired parameter injection (docs/review-notes): input, context, DI services, ct.</summary>
    internal async Task<Result> InvokeHandler(
        OperationDefinition operation, object input, OperationContext context, CancellationToken ct)
    {
        var args = BindParameters(operation.Execute, input, context, ct);
        var invocation = ReflectionCache.Invoker(operation.Execute).Invoke(null, args.AsSpan())
            ?? throw new InvalidOperationException(
                $"TAM004: {operation.Id} Execute returned null — expected Task<Result<...>>.");
        var task = (Task)invocation;
        await task;
        var resultProperty = task.GetType().GetProperty("Result")
            ?? throw new InvalidOperationException(
                $"TAM004: {operation.Id} Execute must return Task<Result> or Task<Result<T>>, not a plain Task.");
        return (Result)resultProperty.GetValue(task)!;
    }

    /// <summary>Runs ALL of the OPERATION's derivations (docs/40) and merges their results — the ONE
    /// place both resolve and submit evaluate derivations, so their authoritative outputs
    /// (requiredness, membership, blocking findings) can never disagree. Every derivation runs on
    /// every call: resolve returns COMPLETE field state, so an incremental run over only the
    /// dependencies of the just-changed field would let resolve report a stale requiredness that
    /// submit (which always runs them all) then contradicts (Sol re-review, Finding 4). A delta
    /// protocol — resolve returns only changed field states + the client merges — is the future
    /// optimization; until then, correctness over the round-trip cost.</summary>
    public async Task<DerivationResult> RunDerivationsAsync(
        OperationDefinition operation, object input, OperationContext context, CancellationToken ct,
        IReadOnlySet<string>? changedFields = null)
    {
        // Derivations are structurally READ-ONLY (Sol re-review, Finding 3) for the WHOLE evaluation,
        // shared by resolve and submit. The scope flag makes the DbContext's write-guard interceptor
        // reject any durable write (SaveChanges / ExecuteUpdate / raw SQL) a derivation attempts, on
        // every exit path. The finally also discards any tracked-but-unsaved write and always restores
        // the flag — so a throw mid-run (a write rejection, DER008) can't leave the shared context
        // dirty or read-only.
        var db = dbResolver(services);
        var scope = services.GetService<TenantScope>();
        var priorReadOnly = scope?.DerivationReadOnly ?? false;
        if (scope is not null) scope.DerivationReadOnly = true;
        try
        {
            var merged = DerivationResult.Empty;
            foreach (var derivation in model.DerivationsForOperation(operation.Id))
            {
                var args = BindParameters(derivation.Method, input, context, ct, changedFields);
                var invocation = derivation.Method.Invoke(null, args)!;
                var result = invocation is Task<DerivationResult> task ? await task : (DerivationResult)invocation;
                merged = merged.Merge(result);
            }

            // A tracked-but-unsaved write (an Add the interceptor never saw) is not durable, but it is
            // still a derivation touching write state — fail closed. The finally discards it so it
            // can't flush on the shared context.
            if (db.ChangeTracker.Entries().Any(e =>
                    e.State is EntityState.Added or EntityState.Modified or EntityState.Deleted))
                throw new InvalidOperationException(
                    $"DER007: a derivation of '{operation.Id}' mutated tracked state. Derivations compute "
                    + "input admissibility and must be read-only — move any write into the operation handler.");

            // Exactly ONE representable candidate contract per field (Sol re-review round 4, Finding
            // 3, broadening the earlier lookup-only DER008). The client can render one candidate
            // source per field, but submit enforces EVERY lookup AND every closed-option set — so two
            // lookups, two closed sets, a lookup + closed set, or an advisory-option set + a lookup
            // would show one thing and enforce another. Advisory options that BACK a RequireOneOf
            // (which also writes the Options dict for display) are the same source, not a conflict.
            var closedByField = merged.ClosedOptions.GroupBy(c => c.Field)
                .ToDictionary(g => g.Key, g => g.Count());
            var candidateFields = merged.Lookups.Select(l => l.Field)
                .Concat(merged.ClosedOptions.Select(c => c.Field))
                .Concat(merged.Options.Keys)
                .Distinct();
            foreach (var candidateField in candidateFields)
            {
                var lookups = merged.Lookups.Count(l => l.Field == candidateField);
                var closed = closedByField.GetValueOrDefault(candidateField);
                // Options is now purely ADVISORY (RequireOneOf no longer writes it, Finding 2), so any
                // entry is a distinct source: RequireOneOf(...).AddOptions(...) → closed 1 + advisory 1.
                var advisory = merged.Options.ContainsKey(candidateField) ? 1 : 0;
                if (lookups + closed + advisory > 1)
                    throw new InvalidOperationException(
                        $"DER008: operation '{operation.Id}' produced {lookups + closed + advisory} "
                        + $"candidate sources for field '{candidateField}' (lookup / closed options / "
                        + "advisory options). A resolved field must have exactly ONE representable "
                        + "candidate contract — resolve shows one, but submit enforces every lookup and "
                        + "closed set.");
            }

            return merged;
        }
        finally
        {
            if (scope is not null) scope.DerivationReadOnly = priorReadOnly;
            if (db.ChangeTracker.Entries().Any(e =>
                    e.State is EntityState.Added or EntityState.Modified or EntityState.Deleted))
                db.ChangeTracker.Clear();
        }
    }

    /// <summary>Wire-name field emptiness, unwrapping a change set — the shared check behind form
    /// tightening (<see cref="SelectedFormRequired"/>) and operation-owned requiredness.</summary>
    private static bool FieldEmpty(OperationDefinition operation, object input, string wireName)
    {
        var field = operation.InputFields.FirstOrDefault(f => f.WireName == wireName);
        if (field is null) return false;
        var value = ReflectionCache.Property(operation.InputType, field.MemberName).GetValue(input);
        var effective = field.IsChangeSet && value is not null
            ? ReflectionCache.Property(value.GetType(), "Value").GetValue(value)
            : value;
        return IsEmpty(effective);
    }

    /// <summary>The submitted value's scalar, read from the DESERIALIZED input (Sol re-review,
    /// Finding 9): unwraps a Change&lt;T&gt; to its submitted value and the semantic wrapper to its
    /// scalar, so an edit field ({original,value} on the wire) yields the actual value, not the
    /// change object. Null when the field is absent or unset.</summary>
    private static object? EffectiveScalar(OperationDefinition operation, object input, string wireName)
    {
        var field = operation.InputFields.FirstOrDefault(f => f.WireName == wireName);
        if (field is null) return null;
        var value = ReflectionCache.Property(operation.InputType, field.MemberName).GetValue(input);
        var effective = field.IsChangeSet && value is not null
            ? ReflectionCache.Property(value.GetType(), "Value").GetValue(value)
            : value;
        return ValueWrapper.Unwrap(effective);
    }

    /// <summary>The effective value of a field for PORTABLE PREDICATE evaluation (Sol re-review
    /// round 6, F2): unwraps a Change&lt;T&gt; to its submitted .Value so an edit field — whose wire
    /// shape is {original,value} and whose deserialized property is a Change&lt;T&gt; — feeds a
    /// predicate the same scalar a create form's raw field would. The SEMANTIC wrapper is
    /// deliberately left ON: <see cref="PxBinary"/>.Normalize unwraps it at evaluation, and a create
    /// form passes the wrapper through unchanged, so edit and create forms evaluate the identical
    /// shape. This is the ONE effective-value accessor every server portable predicate reads through
    /// (resolve field state, selected-form requiredness). Null when the field is absent.</summary>
    internal static object? EffectiveFieldValue(OperationDefinition operation, object input, string wireName)
    {
        var field = operation.InputFields.FirstOrDefault(f => f.WireName == wireName);
        if (field is null) return null;
        var value = ReflectionCache.Property(operation.InputType, field.MemberName).GetValue(input);
        return field.IsChangeSet && value is not null
            ? ReflectionCache.Property(value.GetType(), "Value").GetValue(value)
            : value;
    }

    /// <summary>The submitted lookup key as a wire string for the membership Exists.</summary>
    private static string? LookupKey(OperationDefinition operation, object input, string wireName) =>
        EffectiveScalar(operation, input, wireName) switch
        {
            null => null,
            Guid g => g == Guid.Empty ? null : g.ToString(),
            string s => s,
            var scalar => scalar.ToString(),
        };

    private static readonly IReadOnlySet<string> NoChangedFields = new HashSet<string>(StringComparer.Ordinal);

    internal object?[] BindParameters(
        MethodInfo method, object? input, OperationContext context, CancellationToken ct,
        IReadOnlySet<string>? changedFields = null)
    {
        return ReflectionCache.Parameters(method).Select(p =>
        {
            if (p.ParameterType == typeof(CancellationToken)) return (object?)ct;
            if (p.ParameterType == typeof(OperationContext)) return context;
            if (input is not null && p.ParameterType.IsInstanceOfType(input) && p.Position == 0) return input;
            if (p.ParameterType == typeof(DerivationContext))
                return new DerivationContext(context, changedFields ?? NoChangedFields);
            return services.GetService(p.ParameterType)
                ?? throw new InvalidOperationException(
                    $"DI001: cannot bind parameter '{p.Name}' ({p.ParameterType.Name}) of {method.DeclaringType?.Name}.{method.Name}.");
        }).ToArray();
    }

    private static object? Output(Result result) =>
        result.GetType() is { IsGenericType: true } t && t.GetGenericTypeDefinition() == typeof(Result<>)
            ? t.GetProperty("Output")!.GetValue(result)
            : null;

    private OperationResponse Fail(OperationContext context, params Finding[] findings) =>
        new(null, Resolve(findings, context), [], null, null);

    private IReadOnlyList<Finding> Resolve(IEnumerable<Finding> findings, OperationContext context) =>
        findings.Select(f => model.Locales.Resolve(f, context.Culture)).ToList();
}

/// <summary>Context available to server derivations; services arrive via parameter injection. Note
/// (docs/40): resolve sends every initialized Change&lt;T&gt; field — including untouched ones — so a
/// non-null Change&lt;T&gt; here does NOT mean the user changed it. Read the effective value (.Value),
/// not wrapper presence, and key off <see cref="WasChanged"/> when change-membership matters.</summary>
public sealed record DerivationContext(OperationContext Operation, IReadOnlySet<string> ChangedFields)
{
    /// <summary>Was this field part of the caller's change set (Sol re-review round 6, F3)? At
    /// submit the set is the body's present fields; at resolve it is the fields the client has
    /// touched. This is the RELIABLE change signal — wrapper presence is not, since resolve sends
    /// every initialized Change&lt;T&gt;, touched or not. Matched by wire (camel) name.</summary>
    public bool WasChanged(string field) => ChangedFields.Contains(Naming.Camel(field));
}
